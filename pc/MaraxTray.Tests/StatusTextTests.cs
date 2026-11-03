using System.Net;
using Xunit;

namespace MaraxTray.Tests;

public class StatusTextTests
{
    private static MachineView View(MachinePhase phase, MaraxPacket? packet = null) =>
        new(phase, packet, IPAddress.Parse("192.168.1.50"));

    private static readonly MaraxPacket Idle = new()
    {
        Online = true, HxTemp = 93, SteamTemp = 122, SteamTarget = 124, Heating = true,
        Mode = "+", Firmware = "1.22", Rssi = -61,
    };

    [Fact]
    public void TooltipShowsTemperatureHeatingStateAndPump()
    {
        Assert.Equal(
            "MaraX: Aufgeheizt\nHX 93 °C, Dampf 122 °C\nHeizelement an, Pumpe aus",
            StatusText.Tooltip(View(MachinePhase.Ready, Idle)));
    }

    [Fact]
    public void TooltipShowsStillHeating()
    {
        Assert.StartsWith("MaraX: Heizt noch auf\n", StatusText.Tooltip(View(MachinePhase.Heating, Idle)));
    }

    [Fact]
    public void TooltipShowsRunningPumpWithShotTime()
    {
        var running = Idle with { Pump = true, ShotSeconds = 12 };

        Assert.EndsWith("Pumpe läuft (12 s)", StatusText.Tooltip(View(MachinePhase.Ready, running)));
    }

    [Fact]
    public void MissingWaterGoesFirst()
    {
        var view = View(MachinePhase.Heating, Idle) with { WaterMissing = true };

        Assert.StartsWith("MaraX: Wassertank prüfen\n", StatusText.Tooltip(view));
        Assert.Equal("Wassertank prüfen", StatusText.Details(view)[0]);
    }

    [Theory]
    [InlineData(75, "1:15 min")]
    [InlineData(1112, "18:32 min")]
    [InlineData(4320, "1:12 h")]
    public void DurationsStayReadable(int seconds, string expected)
    {
        Assert.Equal(expected, StatusText.Duration(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void TooltipLeavesOutTheShotTimeWithoutReedSensor()
    {
        var running = Idle with { Pump = true };

        Assert.EndsWith(", Pumpe läuft", StatusText.Tooltip(View(MachinePhase.Ready, running)));
    }

    [Theory]
    [InlineData((int)MachinePhase.NoData, "MaraX: Keine Daten vom ESP")]
    [InlineData((int)MachinePhase.MachineOff, "MaraX: Maschine aus")]
    [InlineData((int)MachinePhase.UnknownFormat, "MaraX: Datenformat unbekannt")]
    public void TooltipForPhasesWithoutMachineData(int phase, string expected)
    {
        Assert.Equal(expected, StatusText.Tooltip(View((MachinePhase)phase, new MaraxPacket { RawLine = "abc" })));
    }

    [Fact]
    public void LongestTooltipFitsIntoTheWindowsLimit()
    {
        var longest = Idle with { HxTemp = 200, SteamTemp = 200, Pump = true, ShotSeconds = 2147483 };

        Assert.True(StatusText.Tooltip(View(MachinePhase.Heating, longest)).Length <= 127);
    }

    [Fact]
    public void DetailsListEverything()
    {
        var packet = Idle with { BoostCountdown = 840, BadLines = 3 };

        Assert.Equal(
            new[]
            {
                "Aufgeheizt",
                "Wärmetauscher (HX): 93 °C",
                "Dampfkessel: 122 °C (Soll 124 °C)",
                "Heizelement: an",
                "Pumpe aus",
                "Boost: 14:00 min",
                "Firmware 1.22, Modus +",
                "ESP: 192.168.1.50, -61 dBm",
                "Verworfene Zeilen: 3",
            },
            StatusText.Details(View(MachinePhase.Ready, packet)));
    }

    [Fact]
    public void DetailsPointToTheWaterTankWithoutTarget()
    {
        var packet = Idle with { SteamTarget = null };

        Assert.Contains(StatusText.Details(View(MachinePhase.Heating, packet)), line => line.Contains("Wassertank"));
    }

    [Fact]
    public void DetailsShowTheUnreadableLine()
    {
        var packet = new MaraxPacket { RawLine = "Z,unknown,format" };

        Assert.Contains("Letzte Zeile: Z,unknown,format", StatusText.Details(View(MachinePhase.UnknownFormat, packet)));
    }

    [Fact]
    public void DetailsWithoutAnyPacket()
    {
        Assert.Equal(new[] { "Keine Daten vom ESP" }, StatusText.Details(new MachineView(MachinePhase.NoData, null, null)));
    }
}
