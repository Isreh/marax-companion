using System.Net;

namespace MaraxTray;

internal enum MachinePhase
{
    NoData,
    MachineOff,
    UnknownFormat,
    Heating,
    Ready,
}

internal sealed record MachineView(MachinePhase Phase, MaraxPacket? Packet, IPAddress? Source, bool WaterMissing = false)
{
    public bool Pump => Phase != MachinePhase.NoData && Packet is { Pump: true };
}

// Turns the packets of the ESP into the state shown in the tray.
internal sealed class MachineTracker(TraySettings settings)
{
    // a single garbled line must not switch the state
    private const int RequiredHotPackets = 3;

    // While the machine fills its boiler, after power on for example, it sends a code instead of
    // the target and runs the pump. A code without the pump means it can't fill.
    private static readonly TimeSpan WaterDelay = TimeSpan.FromSeconds(20);

    private MaraxPacket? lastPacket;
    private IPAddress? lastSource;
    private DateTime lastPacketTime;
    private int hotPackets;
    private bool ready;
    private DateTime? noTargetSince;
    private bool waterMissing;

    public void Update(MaraxPacket packet, IPAddress? source, DateTime now)
    {
        lastPacket = packet;
        lastSource = source;
        lastPacketTime = now;

        if (!packet.Online)
        {
            Reset();
            return;
        }

        if (ready)
        {
            if (packet.HxTemp < settings.CooledDownHxTemp)
            {
                ready = false;
                hotPackets = 0;
            }
        }
        else
        {
            hotPackets = IsHot(packet) ? hotPackets + 1 : 0;
            ready = hotPackets >= RequiredHotPackets;
        }

        if (packet.SteamTarget is null && !packet.Pump)
        {
            noTargetSince ??= now;
        }
        else
        {
            noTargetSince = null;
        }
        waterMissing = noTargetSince is DateTime since && now - since >= WaterDelay;
    }

    public MachineView Current(DateTime now)
    {
        if (lastPacket is null || now - lastPacketTime > TimeSpan.FromSeconds(settings.TimeoutSeconds))
        {
            Reset();
            return new MachineView(MachinePhase.NoData, lastPacket, lastSource);
        }

        MachinePhase phase;
        if (lastPacket.Online)
        {
            phase = ready ? MachinePhase.Ready : MachinePhase.Heating;
        }
        else
        {
            phase = lastPacket.RawLine.Length > 0 ? MachinePhase.UnknownFormat : MachinePhase.MachineOff;
        }
        return new MachineView(phase, lastPacket, lastSource, waterMissing);
    }

    private bool IsHot(MaraxPacket packet) =>
        packet.HxTemp >= settings.ReadyHxTemp
        && packet.SteamTarget is int target
        && packet.SteamTemp >= target - settings.ReadySteamTolerance;

    private void Reset()
    {
        ready = false;
        hotPackets = 0;
        waterMissing = false;
        noTargetSince = null;
    }
}
