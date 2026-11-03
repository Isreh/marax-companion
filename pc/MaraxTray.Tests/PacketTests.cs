using System.Text;
using Xunit;

namespace MaraxTray.Tests;

public class PacketTests
{
    private const string Valid =
        """{"dev":"marax","v":1,"online":true,"mode":"+","fw":"1.22","hx":93,"steam":122,"target":124,"boost":840,"heat":true,"pump":false,"shot":0,"rssi":-61,"uptime":1,"bad":2,"raw":"x"}""";

    private static MaraxPacket? Parse(string json) => MaraxPacket.TryParse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void ReadsAllFields()
    {
        MaraxPacket packet = Parse(Valid)!;

        Assert.True(packet.Online);
        Assert.Equal("+", packet.Mode);
        Assert.Equal("1.22", packet.Firmware);
        Assert.Equal(93, packet.HxTemp);
        Assert.Equal(122, packet.SteamTemp);
        Assert.Equal(124, packet.SteamTarget);
        Assert.Equal(840, packet.BoostCountdown);
        Assert.True(packet.Heating);
        Assert.False(packet.Pump);
        Assert.Equal(-61, packet.Rssi);
        Assert.Equal(2, packet.BadLines);
    }

    [Fact]
    public void TargetMayBeNull()
    {
        MaraxPacket packet = Parse(Valid.Replace("\"target\":124", "\"target\":null"))!;

        Assert.Null(packet.SteamTarget);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"dev":"marax"}""")]
    [InlineData("""{"dev":"other","v":1,"online":false,"pump":false,"shot":0,"rssi":0,"uptime":0,"bad":0,"raw":""}""")]
    [InlineData("""{"dev":"marax","v":2,"online":false,"pump":false,"shot":0,"rssi":0,"uptime":0,"bad":0,"raw":""}""")]
    [InlineData("""{"dev":"marax","v":1,"online":true,"pump":false,"shot":0,"rssi":0,"uptime":0,"bad":0,"raw":""}""")]
    [InlineData("""{"dev":"marax","v":1,"online":"yes","pump":false,"shot":0,"rssi":0,"uptime":0,"bad":0,"raw":""}""")]
    public void RejectsBrokenPackets(string json)
    {
        Assert.Null(Parse(json));
    }

    [Theory]
    [InlineData("\"hx\":93", "\"hx\":999")]
    [InlineData("\"hx\":93", "\"hx\":-1")]
    [InlineData("\"steam\":122", "\"steam\":201")]
    [InlineData("\"target\":124", "\"target\":500")]
    [InlineData("\"boost\":840", "\"boost\":-5")]
    [InlineData("\"hx\":93", "\"hx\":\"93\"")]
    public void RejectsImplausibleValues(string original, string replacement)
    {
        Assert.Null(Parse(Valid.Replace(original, replacement)));
    }

    [Fact]
    public void RejectsOversizedPackets()
    {
        Assert.Null(Parse(Valid.Replace("\"raw\":\"x\"", $"\"raw\":\"{new string('x', 2000)}\"")));
    }

    // protocol_samples.jsonl is the real output of formatMaraxStatus in timer/marax_protocol.h.
    [Fact]
    public void ReadsThePacketsTheFirmwareProduces()
    {
        string[] lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "protocol_samples.jsonl"));
        MaraxPacket[] packets = lines.Select(line => Parse(line)!).ToArray();

        Assert.Equal(7, packets.Length);
        Assert.All(packets, Assert.NotNull);

        Assert.Equal((70, 112, 124, 900, true), (packets[0].HxTemp, packets[0].SteamTemp, packets[0].SteamTarget, packets[0].BoostCountdown, packets[0].Heating));
        Assert.Equal("+", packets[0].Mode);
        Assert.False(packets[1].Heating);
        Assert.Equal((true, 12), (packets[2].Pump, packets[2].ShotSeconds));
        Assert.Equal(("C", "1.06", 97), (packets[3].Mode, packets[3].Firmware, packets[3].HxTemp));
        Assert.Null(packets[3].Line!.Brewing);
        Assert.Equal("D07", packets[4].Line!.TargetText);
        Assert.Null(packets[4].SteamTarget);
        Assert.False(packets[5].Online);
        Assert.Equal("", packets[5].RawLine);
        Assert.False(packets[6].Online);
        Assert.Equal("Z,unknown,format", packets[6].RawLine);
    }
}
