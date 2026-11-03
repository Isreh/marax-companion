namespace MaraxTray;

// What a pump run was. The machine reports in the last field of its line whether the brew group
// is in use (the lever is up). A run without that only refilled the steam boiler, either on its
// own or because hot water was drawn from it.
internal static class PumpRunKind
{
    public const string Espresso = "espresso";
    public const string Flush = "flush";
    public const string Water = "water";
    public const string Refill = "refill";

    // a single flipped bit must not turn hot water into an espresso
    private const int RequiredBrewPackets = 2;

    public static string Of(long durationMs, long brewPackets, long validPackets, TraySettings settings)
    {
        long minimum = settings.MinShotSeconds * 1000L;
        bool shotLength = durationMs >= minimum && durationMs <= settings.MaxShotSeconds * 1000L;

        if (brewPackets >= RequiredBrewPackets && shotLength)
        {
            return Espresso;
        }
        if (brewPackets > 0)
        {
            return Flush;
        }
        if (validPackets == 0)
        {
            // V1 machines don't report the brew group, only the length of the run is left
            return shotLength ? Espresso : Refill;
        }
        return durationMs >= minimum ? Water : Refill;
    }

    public static string Label(string kind) => kind switch
    {
        Espresso => "Espresso",
        Flush => "Spülen",
        Water => "Heißwasser",
        _ => "Nachfüllen",
    };
}
