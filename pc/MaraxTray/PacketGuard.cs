namespace MaraxTray;

// Older ESP firmware accepts a few garbled lines of the machine as valid, and the pump disturbs
// the serial line a lot. The readings of a packet only count when its raw line is a clean line of
// the machine, otherwise the last clean readings stay. Pump and everything else still come through.
internal sealed class PacketGuard
{
    private MaraxPacket? trusted;

    public MaraxPacket Check(MaraxPacket packet)
    {
        if (!packet.Online)
        {
            trusted = null;
            return packet;
        }
        if (packet.Line is not null || trusted is null)
        {
            trusted = packet;
            return packet;
        }

        return packet with
        {
            HxTemp = trusted.HxTemp,
            SteamTemp = trusted.SteamTemp,
            SteamTarget = trusted.SteamTarget,
            BoostCountdown = trusted.BoostCountdown,
            Heating = trusted.Heating,
            Mode = trusted.Mode,
            Firmware = trusted.Firmware,
        };
    }
}
