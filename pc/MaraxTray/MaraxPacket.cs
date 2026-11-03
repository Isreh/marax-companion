using System.Text.Json;

namespace MaraxTray;

// One status packet of the ESP, see formatMaraxStatus in timer/marax_protocol.h.
internal sealed record MaraxPacket
{
    private const int MaxPacketBytes = 1024;
    private const int MaxTemp = 200;

    public bool Online { get; init; }
    public string Mode { get; init; } = "";
    public string Firmware { get; init; } = "";
    public int HxTemp { get; init; }
    public int SteamTemp { get; init; }
    public int? SteamTarget { get; init; }
    public int BoostCountdown { get; init; }
    public bool Heating { get; init; }
    public bool Pump { get; init; }
    public int ShotSeconds { get; init; }
    public int Rssi { get; init; }
    public long Uptime { get; init; }
    public long BadLines { get; init; }
    public string RawLine { get; init; } = "";

    // null when the last line the ESP received was garbled
    public MaraxLine? Line => MaraxLine.TryParse(RawLine);

    // Anyone in the network can send to this port, so a packet is only accepted
    // if it is complete and its values are plausible.
    public static MaraxPacket? TryParse(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0 || data.Length > MaxPacketBytes)
        {
            return null;
        }

        try
        {
            var reader = new Utf8JsonReader(data);
            using JsonDocument document = JsonDocument.ParseValue(ref reader);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || root.GetProperty("dev").GetString() != "marax"
                || root.GetProperty("v").GetInt32() != 1)
            {
                return null;
            }

            var packet = new MaraxPacket
            {
                Online = root.GetProperty("online").GetBoolean(),
                Pump = root.GetProperty("pump").GetBoolean(),
                ShotSeconds = root.GetProperty("shot").GetInt32(),
                Rssi = root.GetProperty("rssi").GetInt32(),
                Uptime = root.GetProperty("uptime").GetInt64(),
                BadLines = root.GetProperty("bad").GetInt64(),
                RawLine = root.GetProperty("raw").GetString() ?? "",
            };
            if (!packet.Online)
            {
                return packet;
            }

            JsonElement target = root.GetProperty("target");
            packet = packet with
            {
                Mode = root.GetProperty("mode").GetString() ?? "",
                Firmware = root.GetProperty("fw").GetString() ?? "",
                HxTemp = root.GetProperty("hx").GetInt32(),
                SteamTemp = root.GetProperty("steam").GetInt32(),
                SteamTarget = target.ValueKind == JsonValueKind.Null ? null : target.GetInt32(),
                BoostCountdown = root.GetProperty("boost").GetInt32(),
                Heating = root.GetProperty("heat").GetBoolean(),
            };

            bool plausible = IsTemp(packet.HxTemp) && IsTemp(packet.SteamTemp)
                && (packet.SteamTarget is null || IsTemp(packet.SteamTarget.Value))
                && packet.BoostCountdown >= 0;
            return plausible ? packet : null;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static bool IsTemp(int value) => value is >= 0 and <= MaxTemp;
}
