using System.Net;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MaraxTray.Tests;

public sealed class HistoryRecorderTests : IDisposable
{
    private static readonly DateTime Start = DateTime.UtcNow.Date.AddHours(6);

    private readonly string folder = Path.Combine(Path.GetTempPath(), "MaraxTrayTests", Guid.NewGuid().ToString("N"));
    private readonly TraySettings settings = new();
    private readonly HistoryStore store;
    private readonly MachineTracker tracker;
    private HistoryRecorder recorder;
    private DateTime now = Start;
    private long uptime = 100_000;

    public HistoryRecorderTests()
    {
        store = new HistoryStore(Path.Combine(folder, "history.db"), settings);
        tracker = new MachineTracker(settings);
        recorder = new HistoryRecorder(store, settings);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(folder, recursive: true);
    }

    // brew null sends no clean line, like a machine that doesn't report the brew group
    private static MaraxPacket Online(int hx = 40, int steam = 60, int? target = 124, bool pump = false, bool? brew = null) => new()
    {
        Online = true, HxTemp = hx, SteamTemp = steam, SteamTarget = target, Pump = pump, Mode = "+", Firmware = "1.22",
        RawLine = brew is bool brewing
            ? $"C1.09,{steam:000},{(target is int t ? t.ToString("000") : "D07")},{hx:000},0000,1,{(brewing ? 1 : 0)}?"
            : "raw",
    };

    private static MaraxPacket Off() => new() { Online = false };

    private void Send(MaraxPacket packet, int seconds = 1)
    {
        for (int i = 0; i < seconds; i++)
        {
            now = now.AddSeconds(1);
            uptime++;
            packet = packet with { Uptime = uptime };
            tracker.Update(packet, IPAddress.Loopback, now);
            recorder.Update(packet, tracker.Current(now), now);
        }
        Assert.Null(recorder.LastError);
    }

    private List<SessionInfo> Sessions() => store.Sessions(0, 100);

    [Fact]
    public void StartAfterTheEspReportedTheMachineOffIsExact()
    {
        Send(Off(), seconds: 5);
        DateTime on = now.AddSeconds(1);

        Send(Online());

        SessionInfo session = Assert.Single(Sessions());
        Assert.True(session.StartExact);
        Assert.Equal(HistoryStore.ToMs(on), session.StartedAt);
        Assert.Equal(40, session.StartHx);
    }

    [Fact]
    public void FirstSeenWhileAlreadyRunningIsMarkedAsInexact()
    {
        Send(Online(hx: 70));

        Assert.False(Assert.Single(Sessions()).StartExact);
    }

    [Fact]
    public void AnEspThatJustStartedDatesTheStartBack()
    {
        uptime = 19;
        Send(Online());

        SessionInfo session = Assert.Single(Sessions());
        Assert.True(session.StartExact);
        Assert.Equal(HistoryStore.ToMs(now.AddSeconds(-20)), session.StartedAt);
        Assert.Equal(true, store.EspFollowsMachine);
    }

    [Fact]
    public void AnEspThatFollowsTheMachineGivesTheStartEvenIfThePcComesLater()
    {
        store.EspFollowsMachine = true;
        recorder = new HistoryRecorder(store, settings);
        uptime = 3599;

        Send(Online(hx: 70));

        SessionInfo session = Assert.Single(Sessions());
        Assert.True(session.StartExact);
        Assert.Equal(HistoryStore.ToMs(now.AddHours(-1)), session.StartedAt);
    }

    [Fact]
    public void AReportedOffShowsThatTheEspRunsOnItsOwn()
    {
        store.EspFollowsMachine = true;
        recorder = new HistoryRecorder(store, settings);

        Send(Off());

        Assert.Equal(false, store.EspFollowsMachine);
    }

    [Fact]
    public void OldOffReportIsNoProofForAnExactStart()
    {
        Send(Off(), seconds: 3);
        now = now.AddHours(8);

        Send(Online(hx: 80));

        Assert.False(Assert.Single(Sessions()).StartExact);
    }

    [Fact]
    public void HeatUpIsTheTimeFromStartToReady()
    {
        Send(Off());
        Send(Online(hx: 30, steam: 50), seconds: 600);
        Send(Online(hx: 92, steam: 124), seconds: 3);

        SessionInfo session = Assert.Single(Sessions());
        Assert.Equal(602, session.HeatupSeconds);
        Assert.True(session.ColdStart);
    }

    [Fact]
    public void HeatUpIsUnknownWhenTheMachineWasFirstSeenHot()
    {
        Send(Online(hx: 93, steam: 124), seconds: 10);

        SessionInfo session = Assert.Single(Sessions());
        Assert.Null(session.ReadyAt);
        Assert.Null(session.HeatupSeconds);
    }

    [Fact]
    public void OnlyTheFirstReadyCounts()
    {
        Send(Off());
        Send(Online(hx: 92, steam: 124), seconds: 3);
        long firstReady = Assert.Single(Sessions()).ReadyAt!.Value;

        Send(Online(hx: 70, steam: 100), seconds: 5);
        Send(Online(hx: 92, steam: 124), seconds: 3);

        Assert.Equal(firstReady, Assert.Single(Sessions()).ReadyAt);
    }

    [Fact]
    public void LongPumpRunIsACoffeeShortOneIsNot()
    {
        Send(Off());
        Send(Online());
        Send(Online(pump: true), seconds: 6);
        Send(Online());
        Send(Online(hx: 93, pump: true), seconds: 28);
        Send(Online());

        SessionInfo session = Assert.Single(Sessions());
        Assert.Equal(1, session.Coffees);
        Assert.Equal(2, session.PumpRuns);
        ShotInfo shot = Assert.Single(store.Shots(0, 100));
        Assert.Equal(28, shot.DurationSeconds);
        Assert.Equal(93, shot.HxStart);
    }

    [Fact]
    public void TheLimitsForACoffeeComeFromTheSettings()
    {
        settings.MinShotSeconds = 3;
        Send(Off());
        Send(Online(pump: true), seconds: 6);
        Send(Online());

        Assert.Equal(1, Assert.Single(Sessions()).Coffees);
    }

    [Fact]
    public void PumpRunRemembersTheLowestTemperature()
    {
        Send(Off());
        Send(Online(hx: 93, pump: true), seconds: 5);
        Send(Online(hx: 86, pump: true), seconds: 5);
        Send(Online(hx: 89, pump: true), seconds: 10);
        Send(Online(hx: 89));

        ShotInfo shot = Assert.Single(store.Shots(0, 100));
        Assert.Equal((93, 86), (shot.HxStart, shot.HxMin));
    }

    [Fact]
    public void PumpRunEndsWhenTheDataStops()
    {
        Send(Off());
        Send(Online(pump: true), seconds: 20);
        now = now.AddMinutes(5);
        Send(Online());

        Assert.Equal(19, Assert.Single(store.Shots(0, 100)).DurationSeconds);
    }

    [Fact]
    public void MachineOffClosesTheSessionAtTheLastMessage()
    {
        Send(Off());
        Send(Online(), seconds: 100);
        DateTime lastOnline = now;

        Send(Off(), seconds: 61);

        SessionInfo session = Assert.Single(Sessions());
        Assert.Equal(HistoryStore.ToMs(lastOnline), session.EndedAt);
        Assert.Equal(99, session.RuntimeSeconds);
    }

    [Fact]
    public void AShortSerialDropoutKeepsTheSession()
    {
        Send(Off());
        Send(Online(), seconds: 10);
        Send(Off(), seconds: 30);
        Send(Online(), seconds: 10);

        Assert.Null(Assert.Single(Sessions()).EndedAt);
    }

    [Fact]
    public void ALongSilenceStartsANewSession()
    {
        Send(Off());
        Send(Online(), seconds: 10);
        DateTime lastOnline = now;
        now = now.AddMinutes(25);

        Send(Online());

        List<SessionInfo> sessions = Sessions();
        Assert.Equal(2, sessions.Count);
        Assert.Equal(HistoryStore.ToMs(lastOnline), sessions[1].EndedAt);
    }

    [Fact]
    public void AShortSilenceKeepsTheSession()
    {
        Send(Off());
        Send(Online(), seconds: 10);
        now = now.AddMinutes(10);

        Send(Online());

        Assert.Single(Sessions());
    }

    [Fact]
    public void WithAnEspThatFollowsTheMachineItsUptimeDecidesAboutTheSession()
    {
        uptime = 10;
        Send(Online());
        now = now.AddHours(1);
        uptime += 3600;

        Send(Online());

        Assert.Single(Sessions());
    }

    [Fact]
    public void RestartOfTheEspStartsANewSession()
    {
        Send(Off());
        Send(Online(), seconds: 10);
        uptime = 5;

        Send(Online());

        Assert.Equal(2, Sessions().Count);
        Assert.Contains(store.Events(0, 100), e => e.Kind == "esp_restart");
    }

    [Fact]
    public void AnEspThatFollowsTheMachineStartsANewSessionWithoutAnyNoise()
    {
        store.EspFollowsMachine = true;
        recorder = new HistoryRecorder(store, settings);
        uptime = 500;
        Send(Online());
        now = now.AddHours(3);
        uptime = 4;

        Send(Online());

        Assert.Equal(2, Sessions().Count);
        Assert.DoesNotContain(store.Events(0, 100), e => e.Kind == "esp_restart");
    }

    [Fact]
    public void AfterAnAppRestartTheOpenSessionContinues()
    {
        Send(Off());
        Send(Online(hx: 92, steam: 124), seconds: 3);
        long? ready = Sessions()[0].ReadyAt;

        recorder = new HistoryRecorder(store, settings);
        Send(Online(hx: 92, steam: 124), seconds: 3);

        SessionInfo session = Assert.Single(Sessions());
        Assert.Equal(ready, session.ReadyAt);
    }

    [Fact]
    public void SamplesAreTakenEveryFiveSecondsAndEverySecondWhileThePumpRuns()
    {
        Send(Off());
        Send(Online(), seconds: 18);
        int idle = store.Session(null, 1500)!.Samples.Count;

        // the pump starts between two samples, its first second must not get lost
        Send(Online(pump: true), seconds: 10);

        Assert.Equal(4, idle);
        Assert.Equal(14, store.Session(null, 1500)!.Samples.Count);
    }

    [Fact]
    public void EspressoIsWhereTheMachineReportsTheBrewGroup()
    {
        Send(Off());
        Send(Online(hx: 92, steam: 129, brew: false));
        Send(Online(hx: 92, steam: 129, pump: true, brew: false), seconds: 2);
        Send(Online(hx: 98, steam: 130, pump: true, brew: true), seconds: 23);
        Send(Online(hx: 93, steam: 132, brew: false));

        ShotInfo shot = Assert.Single(store.Shots(0, 100));
        Assert.Equal(PumpRunKind.Espresso, shot.Kind);
        Assert.Equal(1, Assert.Single(Sessions()).Coffees);
        Assert.Contains(store.Events(0, 100), e => e.Kind == "coffee");
    }

    [Fact]
    public void PumpingWithoutTheBrewGroupIsHotWater()
    {
        Send(Off());
        Send(Online(hx: 94, steam: 128, brew: false));
        Send(Online(hx: 94, steam: 124, pump: true, brew: false), seconds: 32);
        Send(Online(hx: 94, steam: 123, brew: false));

        ShotInfo draw = Assert.Single(store.Shots(0, 100));
        Assert.Equal(PumpRunKind.Water, draw.Kind);
        Assert.Equal("Heißwasser", draw.KindLabel);
        Assert.Equal(0, Assert.Single(Sessions()).Coffees);
        Assert.Contains(store.Events(0, 100), e => e.Kind == "hot_water");
        Assert.DoesNotContain(store.Events(0, 100), e => e.Kind == "coffee");
    }

    [Fact]
    public void ShortRunsAreNoDraws()
    {
        Send(Off());
        Send(Online(brew: false));
        Send(Online(pump: true, brew: true), seconds: 4);
        Send(Online(brew: false));
        Send(Online(pump: true, brew: false), seconds: 3);
        Send(Online(brew: false));

        Assert.Empty(store.Shots(0, 100));
        Assert.Equal(2, Assert.Single(Sessions()).PumpRuns);
    }

    [Fact]
    public void GarbledLinesDuringTheRunDoNotCount()
    {
        Send(Off());
        Send(Online(pump: true, brew: true), seconds: 2);
        Send(Online(pump: true), seconds: 20);
        Send(Online(brew: false));

        Assert.Equal(PumpRunKind.Espresso, Assert.Single(store.Shots(0, 100)).Kind);
    }

    [Fact]
    public void WaterEventsCoverStartAndEnd()
    {
        Send(Off());
        Send(Online(target: null), seconds: 25);
        Send(Online(), seconds: 5);

        List<EventInfo> events = store.Events(0, 100);
        Assert.Contains(events, e => e.Kind == "water_missing" && e.Detail == "raw");
        Assert.Contains(events, e => e.Kind == "water_ok");
    }

    [Fact]
    public void OneGarbledLineIsNoWaterEvent()
    {
        Send(Off());
        Send(Online(), seconds: 5);
        Send(Online(target: null));
        Send(Online(), seconds: 5);

        Assert.DoesNotContain(store.Events(0, 100), e => e.Kind.StartsWith("water"));
    }

    [Fact]
    public void AStoppedDatabaseOnlyShowsAsAnError()
    {
        SqliteConnection.ClearAllPools();
        string broken = Path.Combine(folder, "broken.db");
        var brokenStore = new HistoryStore(broken, settings);
        var brokenRecorder = new HistoryRecorder(brokenStore, settings);
        SqliteConnection.ClearAllPools();
        File.WriteAllText(broken, "this is no database anymore, this is no database anymore");
        File.Delete(broken + "-wal");
        File.Delete(broken + "-shm");

        brokenRecorder.Update(Online(), new MachineView(MachinePhase.Heating, Online(), null), now);

        Assert.NotNull(brokenRecorder.LastError);
    }
}

public sealed class HistoryQueryTests : IDisposable
{
    private static readonly DateTime Today = DateTime.Today.AddHours(8).ToUniversalTime();

    private readonly string folder = Path.Combine(Path.GetTempPath(), "MaraxTrayTests", Guid.NewGuid().ToString("N"));
    private readonly TraySettings settings = new();
    private readonly HistoryStore store;

    public HistoryQueryTests()
    {
        store = new HistoryStore(Path.Combine(folder, "history.db"), settings);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(folder, recursive: true);
    }

    private long Session(DateTime start, int hx = 25, int? heatupSeconds = 1200, bool exact = true, int runtimeSeconds = 3600)
    {
        long id = store.AddSession(start, exact, hx, 40, "1.22", start.AddSeconds(runtimeSeconds));
        if (heatupSeconds is int seconds)
        {
            store.SetReady(id, start.AddSeconds(seconds));
        }
        store.CloseSession(id, start.AddSeconds(runtimeSeconds));
        return id;
    }

    private void Run(long session, DateTime start, double seconds) =>
        store.AddPumpRun(new PumpRunRow(session, start, TimeSpan.FromSeconds(seconds), 93, 90, 124, "+"));

    [Fact]
    public void EmptyHistoryGivesEmptyResults()
    {
        SummaryInfo summary = store.Summary(30);

        Assert.Equal(0, summary.CoffeesToday);
        Assert.Null(summary.FirstStartToday);
        Assert.Null(summary.AvgHeatupSeconds);
        Assert.Null(summary.AvgShotSeconds);
        Assert.Null(summary.LastCoffeeAt);
        Assert.Empty(store.Daily(30));
        Assert.Null(store.Session(null, 100));
    }

    [Fact]
    public void SummaryCountsOnlyRealShots()
    {
        long today = Session(Today);
        Run(today, Today.AddMinutes(30), 28);
        Run(today, Today.AddMinutes(31), 5);
        Run(today, Today.AddMinutes(32), 120);
        Run(today, Today.AddMinutes(33), 30);

        SummaryInfo summary = store.Summary(30);

        Assert.Equal(2, summary.CoffeesToday);
        Assert.Equal(2, summary.Coffees);
        Assert.Equal(29, summary.AvgShotSeconds);
        Assert.Equal(HistoryStore.ToMs(Today.AddMinutes(33)), summary.LastCoffeeAt);
    }

    [Fact]
    public void TodayStartsWithTheFirstSessionOfTheDay()
    {
        Session(Today, heatupSeconds: 1000);
        Session(Today.AddHours(3), heatupSeconds: 500);

        SummaryInfo summary = store.Summary(30);

        Assert.Equal(HistoryStore.ToMs(Today), summary.FirstStartToday);
        Assert.Equal(1000, summary.HeatupToday);
        Assert.Equal(7200, summary.RuntimeToday);
    }

    [Fact]
    public void AverageHeatUpOnlyUsesExactColdStarts()
    {
        Session(Today, hx: 25, heatupSeconds: 1000);
        Session(Today.AddHours(1), hx: 25, heatupSeconds: 1400);
        Session(Today.AddHours(2), hx: 75, heatupSeconds: 100);
        Session(Today.AddHours(3), hx: 25, heatupSeconds: 10, exact: false);

        Assert.Equal(1200, store.Summary(30).AvgHeatupSeconds);
    }

    [Fact]
    public void TheDayRangeLimitsEverything()
    {
        long old = Session(Today.AddDays(-20));
        Run(old, Today.AddDays(-20), 28);
        long today = Session(Today);
        Run(today, Today, 28);

        Assert.Equal(1, store.Summary(7).Coffees);
        Assert.Equal(2, store.Summary(30).Coffees);
        Assert.Equal(2, store.Summary(0).Coffees);
        Assert.Single(store.Daily(7));
        Assert.Single(store.Sessions(7, 100));
        Assert.Single(store.Shots(7, 100));
    }

    [Fact]
    public void DailyAndHoursGroupInLocalTime()
    {
        long id = Session(Today);
        Run(id, Today, 28);
        Run(id, Today.AddMinutes(5), 28);

        DayInfo day = Assert.Single(store.Daily(30));
        Assert.Equal(DateTime.Today.ToString("yyyy-MM-dd"), day.Day);
        Assert.Equal(2, day.Coffees);

        HourInfo hour = Assert.Single(store.Hours(30));
        Assert.Equal(((int)DateTime.Today.DayOfWeek, 8, 2), (hour.Weekday, hour.Hour, hour.Coffees));
    }

    [Fact]
    public void SessionDetailThinsOutLongCurvesButKeepsTheEnd()
    {
        long id = Session(Today);
        for (int i = 0; i < 100; i++)
        {
            store.AddSample(id, Today.AddSeconds(i * 5), new MaraxPacket { HxTemp = i, SteamTemp = 50, Mode = "+", RawLine = "x" });
        }

        SessionDetail detail = store.Session(id, 30)!;

        Assert.True(detail.Samples.Count <= 31, $"{detail.Samples.Count} points");
        Assert.Equal(99, detail.Samples[^1].Hx);
    }
}
