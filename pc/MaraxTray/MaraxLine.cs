using System.Globalization;
using System.Text.RegularExpressions;

namespace MaraxTray;

// One line of the machine, taken from the raw text the ESP forwards. Only lines with exactly
// the machine's layout count, by the same rules as parseMaraxLine in timer/marax_protocol.h.
internal sealed partial record MaraxLine(
    string Mode,
    int SteamTemp,
    int? SteamTarget,
    string TargetText,
    int HxTemp,
    int BoostCountdown,
    bool Heating,
    bool? Brewing)
{
    private const int MaxTemp = 200;

    // The ESP replaces the carriage return at the end with a question mark.
    [GeneratedRegex(@"^([A-Za-z+])[0-9A-Za-z.]{1,7},(\d{3}),([0-9A-Z]{3}),(\d{3}),(\d{4}),([01])(?:,([01]))?[? ]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Layout();

    public static MaraxLine? TryParse(string raw)
    {
        Match match = Layout().Match(raw);
        if (!match.Success)
        {
            return null;
        }

        int steam = Number(match.Groups[2]);
        int hx = Number(match.Groups[4]);
        if (steam > MaxTemp || hx > MaxTemp)
        {
            return null;
        }

        string targetText = match.Groups[3].Value;
        int? target = int.TryParse(targetText, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value is > 0 and <= MaxTemp
            ? value
            : null;
        return new MaraxLine(
            match.Groups[1].Value,
            steam,
            target,
            targetText,
            hx,
            Number(match.Groups[5]),
            match.Groups[6].Value == "1",
            match.Groups[7].Success ? match.Groups[7].Value == "1" : null);
    }

    private static int Number(Group group) => int.Parse(group.Value, CultureInfo.InvariantCulture);
}
