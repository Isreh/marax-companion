namespace MaraxTray;

internal static class StatusText
{
    public static string Headline(MachineView view)
    {
        if (view.WaterMissing)
        {
            return "Wassertank prüfen";
        }
        return view.Phase switch
        {
            MachinePhase.NoData => "Keine Daten vom ESP",
            MachinePhase.MachineOff => "Maschine aus",
            MachinePhase.UnknownFormat => "Datenformat unbekannt",
            MachinePhase.Heating => "Heizt noch auf",
            MachinePhase.Ready => "Aufgeheizt",
            _ => "",
        };
    }

    public static string Tooltip(MachineView view)
    {
        string headline = $"MaraX: {Headline(view)}";
        if (view.Phase is not (MachinePhase.Heating or MachinePhase.Ready))
        {
            return headline;
        }

        MaraxPacket packet = view.Packet!;
        return $"{headline}\n"
            + $"HX {packet.HxTemp} °C, Dampf {packet.SteamTemp} °C\n"
            + $"Heizelement {OnOff(packet.Heating)}, {Pump(view)}";
    }

    // The lines at the top of the right-click menu.
    public static IReadOnlyList<string> Details(MachineView view)
    {
        var lines = new List<string> { Headline(view) };
        MaraxPacket? packet = view.Packet;

        if (view.Phase is MachinePhase.Heating or MachinePhase.Ready)
        {
            lines.Add($"Wärmetauscher (HX): {packet!.HxTemp} °C");
            lines.Add(packet.SteamTarget is int target
                ? $"Dampfkessel: {packet.SteamTemp} °C (Soll {target} °C)"
                : $"Dampfkessel: {packet.SteamTemp} °C (kein Sollwert, Wassertank prüfen)");
            lines.Add($"Heizelement: {OnOff(packet.Heating)}");
            lines.Add(Pump(view));
            if (packet.BoostCountdown > 0)
            {
                lines.Add($"Boost: {packet.BoostCountdown / 60}:{packet.BoostCountdown % 60:00} min");
            }
            lines.Add($"Firmware {packet.Firmware}, Modus {packet.Mode}");
        }
        else if (view.Phase == MachinePhase.UnknownFormat)
        {
            lines.Add($"Letzte Zeile: {packet!.RawLine}");
        }

        if (view.Phase != MachinePhase.NoData && packet is not null)
        {
            lines.Add($"ESP: {view.Source}, {packet.Rssi} dBm");
            if (packet.BadLines > 0)
            {
                lines.Add($"Verworfene Zeilen: {packet.BadLines}");
            }
        }
        return lines;
    }

    public static string Duration(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours}:{duration.Minutes:00} h"
        : $"{(int)duration.TotalMinutes}:{duration.Seconds:00} min";

    private static string Pump(MachineView view)
    {
        if (!view.Pump)
        {
            return "Pumpe aus";
        }
        int seconds = view.Packet!.ShotSeconds;
        return seconds > 0 ? $"Pumpe läuft ({seconds} s)" : "Pumpe läuft";
    }

    private static string OnOff(bool on) => on ? "an" : "aus";
}
