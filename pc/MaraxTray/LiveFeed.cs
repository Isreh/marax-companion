namespace MaraxTray;

internal sealed record LiveState
{
    public string Phase { get; init; } = nameof(MachinePhase.NoData);
    public string Headline { get; init; } = "";
    public int? Hx { get; init; }
    public int? Steam { get; init; }
    public int? Target { get; init; }
    public int? Boost { get; init; }
    public bool? Heating { get; init; }
    public bool Pump { get; init; }
    public bool Brewing { get; init; }
    public int ShotSeconds { get; init; }
    public bool WaterMissing { get; init; }
    public int? Rssi { get; init; }
    public string? Firmware { get; init; }
    public string? Mode { get; init; }
    public string? Esp { get; init; }
    public long BadLines { get; init; }
    public long? SessionId { get; init; }
    public long Revision { get; init; }
    public string? HistoryError { get; init; }

    public static LiveState From(MachineView view, HistoryRecorder? recorder)
    {
        var state = new LiveState
        {
            Phase = view.Phase.ToString(),
            Headline = StatusText.Headline(view),
            Pump = view.Pump,
            WaterMissing = view.WaterMissing,
            SessionId = recorder?.SessionId,
            Revision = recorder?.Revision ?? 0,
            HistoryError = recorder?.LastError,
        };
        if (view.Phase == MachinePhase.NoData || view.Packet is not MaraxPacket packet)
        {
            return state;
        }

        state = state with
        {
            Rssi = packet.Rssi,
            Esp = view.Source?.ToString(),
            BadLines = packet.BadLines,
            ShotSeconds = view.Pump ? packet.ShotSeconds : 0,
            Brewing = view.Pump && packet.Line?.Brewing == true,
        };
        if (view.Phase is not (MachinePhase.Heating or MachinePhase.Ready))
        {
            return state;
        }

        return state with
        {
            Hx = packet.HxTemp,
            Steam = packet.SteamTemp,
            Target = packet.SteamTarget,
            Boost = packet.BoostCountdown,
            Heating = packet.Heating,
            Firmware = packet.Firmware,
            Mode = packet.Mode,
        };
    }
}

// Hands the state from the UI thread to the threads of the web server.
internal sealed class LiveFeed
{
    private volatile LiveState current = new();

    public LiveState Current => current;

    public void Publish(LiveState state) => current = state;
}
