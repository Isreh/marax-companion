using Microsoft.Data.Sqlite;

namespace MaraxTray;

// Turns the stream of packets into sessions (machine on to machine off), pump runs and events.
internal sealed class HistoryRecorder
{
    // an ESP that started this recently was switched on together with the machine
    private const int EspBootSeconds = 120;
    private const int SameStartToleranceSeconds = 120;

    // The ESP also reports the machine as gone during a short serial dropout.
    private static readonly TimeSpan OffConfirmation = TimeSpan.FromSeconds(60);

    // slightly below the spacing of the packets, so that no packet is skipped by a few milliseconds
    private static readonly TimeSpan IdleSampleInterval = TimeSpan.FromSeconds(4.8);
    private static readonly TimeSpan PumpSampleInterval = TimeSpan.FromSeconds(0.8);

    private readonly HistoryStore store;
    private readonly TraySettings settings;

    private SessionRow? session;
    private PumpRun? run;
    private bool? espFollowsMachine;
    private long? lastUptime;
    private DateTime? offSince;
    private DateTime lastPacket;
    private DateTime lastOnline;
    private DateTime nextSample;
    private bool lastSamplePump;
    private bool waterMissing;

    public HistoryRecorder(HistoryStore store, TraySettings settings)
    {
        this.store = store;
        this.settings = settings;
        session = store.LoadOpenSession();
        espFollowsMachine = store.EspFollowsMachine;
        lastOnline = session?.LastSeen ?? default;
    }

    public long? SessionId => session?.Id;

    // Changes whenever a session, pump run or event was stored, so that the web page knows when to reload.
    public long Revision { get; private set; }

    public string? LastError { get; private set; }

    public void Update(MaraxPacket packet, MachineView view, DateTime now)
    {
        try
        {
            Record(packet, view, now);
            LastError = null;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            // the tray icon has to keep working when the data can't be stored
            LastError = ex.Message;
        }
    }

    public int? CoffeesToday()
    {
        try
        {
            return store.CoffeesToday();
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private void Record(MaraxPacket packet, MachineView view, DateTime now)
    {
        bool espRestarted = lastUptime is long previous && packet.Uptime < previous;
        lastUptime = packet.Uptime;
        // an ESP that follows the machine restarts with every start of the machine
        if (espRestarted && espFollowsMachine != true)
        {
            AddEvent(now, "esp_restart", "");
        }

        if (run is not null && now - lastOnline > TimeSpan.FromSeconds(settings.TimeoutSeconds))
        {
            FinishRun(lastOnline);
        }

        if (packet.Online)
        {
            RecordOnline(packet, view, now, espRestarted);
        }
        else
        {
            RecordOffline(now);
        }
        lastPacket = now;
    }

    private void RecordOffline(DateTime now)
    {
        // only an ESP that keeps running can report that the machine is off
        if (espFollowsMachine != false)
        {
            espFollowsMachine = false;
            store.EspFollowsMachine = false;
        }

        offSince ??= now;
        if (run is not null)
        {
            FinishRun(lastOnline);
        }
        if (session is not null && now - offSince >= OffConfirmation)
        {
            CloseSession();
        }
    }

    private void RecordOnline(MaraxPacket packet, MachineView view, DateTime now, bool espRestarted)
    {
        if (session is not null && !IsSameSession(packet, now, espRestarted))
        {
            CloseSession();
        }
        session ??= OpenSession(packet, now);
        session = session with { LastSeen = now };
        offSince = null;
        lastOnline = now;

        TrackReady(view, now);
        TrackWater(packet, view, now);
        TrackPump(packet, now);
        AddSample(packet, now);
    }

    private bool IsSameSession(MaraxPacket packet, DateTime now, bool espRestarted)
    {
        if (espRestarted)
        {
            return false;
        }
        if (espFollowsMachine == true)
        {
            DateTime espStart = now - TimeSpan.FromSeconds(packet.Uptime);
            return (espStart - session!.StartedAt).Duration() <= TimeSpan.FromSeconds(SameStartToleranceSeconds);
        }
        return now - session!.LastSeen <= TimeSpan.FromMinutes(settings.SessionGapMinutes);
    }

    private SessionRow OpenSession(MaraxPacket packet, DateTime now)
    {
        bool reportedOff = offSince is not null && now - lastPacket <= TimeSpan.FromSeconds(settings.TimeoutSeconds);
        DateTime startedAt = now;
        bool exact = reportedOff;

        if (!reportedOff)
        {
            if (packet.Uptime < EspBootSeconds && espFollowsMachine != true)
            {
                espFollowsMachine = true;
                store.EspFollowsMachine = true;
            }
            if (espFollowsMachine == true)
            {
                startedAt = now - TimeSpan.FromSeconds(packet.Uptime);
                exact = true;
            }
        }

        long id = store.AddSession(startedAt, exact, packet.HxTemp, packet.SteamTemp, packet.Firmware, now);
        AddEvent(startedAt, "machine_on", exact ? $"HX {packet.HxTemp} °C" : $"Erst jetzt gesehen, HX {packet.HxTemp} °C");
        return new SessionRow(id, startedAt, exact, packet.HxTemp, null, now);
    }

    private void CloseSession()
    {
        if (run is not null)
        {
            FinishRun(lastOnline);
        }
        SessionRow closed = session!;
        session = null;
        store.CloseSession(closed.Id, closed.LastSeen);
        AddEvent(closed.LastSeen, "machine_off", $"nach {StatusText.Duration(closed.LastSeen - closed.StartedAt)}");
    }

    private void TrackReady(MachineView view, DateTime now)
    {
        if (view.Phase != MachinePhase.Ready || session!.ReadyAt is not null)
        {
            return;
        }
        // seen for the first time when it was already hot: nobody knows when it got there
        if (!session.StartExact && session.StartHx >= settings.ReadyHxTemp)
        {
            return;
        }

        session = session with { ReadyAt = now };
        store.SetReady(session.Id, now);
        AddEvent(now, "ready", session.StartExact ? $"nach {StatusText.Duration(now - session.StartedAt)}" : "Startzeit ungenau");
    }

    private void TrackWater(MaraxPacket packet, MachineView view, DateTime now)
    {
        if (view.WaterMissing != waterMissing)
        {
            waterMissing = view.WaterMissing;
            AddEvent(now, waterMissing ? "water_missing" : "water_ok", waterMissing ? packet.RawLine : "");
        }
    }

    private void TrackPump(MaraxPacket packet, DateTime now)
    {
        if (!packet.Pump)
        {
            if (run is not null)
            {
                FinishRun(now);
            }
            return;
        }

        run ??= new PumpRun(session!.Id, now, packet.HxTemp, packet.SteamTemp, packet.Mode);
        run.HxMin = Math.Min(run.HxMin, packet.HxTemp);
        if (packet.Line?.Brewing is bool brewing)
        {
            run.ValidPackets++;
            run.BrewPackets += brewing ? 1 : 0;
        }
    }

    private void FinishRun(DateTime end)
    {
        PumpRun finished = run!;
        run = null;
        TimeSpan duration = end - finished.StartedAt;
        store.AddPumpRun(new PumpRunRow(
            finished.SessionId, finished.StartedAt, duration, finished.HxStart, finished.HxMin, finished.SteamStart, finished.Mode,
            finished.BrewPackets, finished.ValidPackets));
        Revision++;

        string kind = PumpRunKind.Of((long)duration.TotalMilliseconds, finished.BrewPackets, finished.ValidPackets, settings);
        if (kind == PumpRunKind.Espresso)
        {
            AddEvent(finished.StartedAt, "coffee", $"{duration.TotalSeconds:0} s, HX {finished.HxStart} °C");
        }
        else if (kind == PumpRunKind.Water)
        {
            AddEvent(finished.StartedAt, "hot_water", $"{duration.TotalSeconds:0} s");
        }
    }

    private void AddSample(MaraxPacket packet, DateTime now)
    {
        if (now < nextSample && packet.Pump == lastSamplePump)
        {
            return;
        }
        nextSample = now + (packet.Pump ? PumpSampleInterval : IdleSampleInterval);
        lastSamplePump = packet.Pump;
        store.AddSample(session!.Id, now, packet);
    }

    private void AddEvent(DateTime at, string kind, string detail)
    {
        store.AddEvent(at, kind, detail);
        Revision++;
    }

    private sealed class PumpRun(long sessionId, DateTime startedAt, int hxStart, int steamStart, string mode)
    {
        public long SessionId { get; } = sessionId;
        public DateTime StartedAt { get; } = startedAt;
        public int HxStart { get; } = hxStart;
        public int SteamStart { get; } = steamStart;
        public string Mode { get; } = mode;
        public int HxMin { get; set; } = hxStart;
        public int BrewPackets { get; set; }
        public int ValidPackets { get; set; }
    }
}
