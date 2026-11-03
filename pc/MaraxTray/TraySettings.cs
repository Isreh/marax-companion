using System.Text.Json;

namespace MaraxTray;

internal sealed class TraySettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public int Port { get; set; } = 4210;
    public string ListenAddress { get; set; } = "0.0.0.0";

    // The machine counts as heated up once the heat exchanger has reached ReadyHxTemp
    // and the steam boiler is within ReadySteamTolerance of its target.
    public int ReadyHxTemp { get; set; } = 90;
    public int ReadySteamTolerance { get; set; } = 2;

    // A shot pulls the temperature down for a moment, so it only counts as
    // heating up again below this value.
    public int CooledDownHxTemp { get; set; } = 80;

    // long enough for the ESP to reconnect to the WiFi without losing the state
    public int TimeoutSeconds { get; set; } = 10;

    // Shorter pump runs are the machine refilling its boiler or a flush, longer ones are no shot either.
    public int MinShotSeconds { get; set; } = 15;
    public int MaxShotSeconds { get; set; } = 90;

    // Below this heat exchanger temperature a start counts as a cold start for the average heat-up time.
    public int ColdStartHxTemp { get; set; } = 50;

    // Without a report that the machine is off, it counts as the same session up to this gap.
    public int SessionGapMinutes { get; set; } = 20;

    public int WebPort { get; set; } = 8420;

    // 0.0.0.0 makes the page reachable in the home network, 127.0.0.1 only on this PC.
    public string WebListenAddress { get; set; } = "0.0.0.0";

    // empty means the default location next to the settings
    public string HistoryPath { get; set; } = "";

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MaraxTray", "settings.json");

    public static TraySettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<TraySettings>(File.ReadAllText(path), JsonOptions) ?? new TraySettings();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // a broken file must not keep the tray icon from starting, and is left for the user to fix
            return new TraySettings();
        }

        var settings = new TraySettings();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the defaults work without the file
        }
        return settings;
    }
}
