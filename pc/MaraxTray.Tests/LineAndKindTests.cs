using Xunit;

namespace MaraxTray.Tests;

public class MaraxLineTests
{
    [Fact]
    public void ReadsARealLineWithTheBrewGroup()
    {
        MaraxLine line = MaraxLine.TryParse("C1.09,129,136,092,0000,1,1?")!;

        Assert.Equal(("C", 129, 136, 92, 0, true), (line.Mode, line.SteamTemp, line.SteamTarget, line.HxTemp, line.BoostCountdown, line.Heating));
        Assert.True(line.Brewing);
    }

    [Fact]
    public void ReadsTheCodeWhileTheBoilerFills()
    {
        MaraxLine line = MaraxLine.TryParse("C1.09,022,D07,022,0000,0,0?")!;

        Assert.Null(line.SteamTarget);
        Assert.Equal("D07", line.TargetText);
        Assert.False(line.Brewing);
    }

    [Fact]
    public void V1LinesHaveNoBrewGroup()
    {
        Assert.Null(MaraxLine.TryParse("C1.06,116,124,093,0840,1")!.Brewing);
    }

    // all of them seen on a real machine while the pump ran
    [Theory]
    [InlineData("")]
    [InlineData("???b?b?")]
    [InlineData("C1.09,127,126?094,0000,0,0?")]
    [InlineData("?1.09,127,126,094,0000,0,0?")]
    [InlineData("C1.09,127,126,094,0000?0,0?")]
    [InlineData("C1.09,022,D07,022,0?C1.09,022,D")]
    [InlineData("C1.09,022,D07,022,0000,0,0?C1.0")]
    [InlineData("C1.0?,138,136,090,0000,?")]
    [InlineData("C1.09,?31,128,095,0000,0,0?")]
    [InlineData("C1.09,301,128,095,0000,0,0")]
    public void RejectsGarbledLines(string raw)
    {
        Assert.Null(MaraxLine.TryParse(raw));
    }
}

public class PacketGuardTests
{
    private readonly PacketGuard guard = new();

    private static MaraxPacket Packet(int hx, string raw, int? target = 124, bool pump = false) =>
        new() { Online = true, HxTemp = hx, SteamTemp = 127, SteamTarget = target, Pump = pump, Mode = "C", Firmware = "1.09", RawLine = raw };

    [Fact]
    public void CleanLinesPassUnchanged()
    {
        MaraxPacket packet = Packet(94, "C1.09,127,124,094,0000,0,0?");

        Assert.Same(packet, guard.Check(packet));
    }

    [Fact]
    public void ReadingsOfAGarbledLineAreReplacedButThePumpStays()
    {
        guard.Check(Packet(94, "C1.09,127,124,094,0000,0,0?"));

        MaraxPacket checkedPacket = guard.Check(Packet(0, "C1.09,127,126?094,0000,0,0?", target: null, pump: true));

        Assert.Equal(94, checkedPacket.HxTemp);
        Assert.Equal(124, checkedPacket.SteamTarget);
        Assert.True(checkedPacket.Pump);
    }

    [Fact]
    public void TheNextCleanLineIsTrustedAgain()
    {
        guard.Check(Packet(94, "C1.09,127,124,094,0000,0,0?"));
        guard.Check(Packet(0, "???b?b?"));

        Assert.Equal(96, guard.Check(Packet(96, "C1.09,127,124,096,0000,0,0?")).HxTemp);
    }

    [Fact]
    public void MachineOffForgetsTheLastReadings()
    {
        guard.Check(Packet(94, "C1.09,127,124,094,0000,0,0?"));
        guard.Check(new MaraxPacket { Online = false });

        Assert.Equal(30, guard.Check(Packet(30, "???")).HxTemp);
    }
}

public class PumpRunKindTests
{
    private readonly TraySettings settings = new();

    [Theory]
    [InlineData(25300, 12, 14, PumpRunKind.Espresso)]
    [InlineData(37400, 20, 24, PumpRunKind.Espresso)]
    [InlineData(19200, 0, 12, PumpRunKind.Water)]
    [InlineData(32500, 0, 20, PumpRunKind.Water)]
    [InlineData(4400, 1, 3, PumpRunKind.Flush)]
    [InlineData(2500, 0, 2, PumpRunKind.Refill)]
    [InlineData(30000, 1, 20, PumpRunKind.Flush)]
    [InlineData(120000, 50, 60, PumpRunKind.Flush)]
    [InlineData(28000, 0, 0, PumpRunKind.Espresso)]
    [InlineData(5000, 0, 0, PumpRunKind.Refill)]
    [InlineData(120000, 0, 0, PumpRunKind.Refill)]
    public void ClassifiesTheRealRuns(long durationMs, long brew, long valid, string expected)
    {
        Assert.Equal(expected, PumpRunKind.Of(durationMs, brew, valid, settings));
    }

    [Fact]
    public void LabelsAreGerman()
    {
        Assert.Equal("Heißwasser", PumpRunKind.Label(PumpRunKind.Water));
        Assert.Equal("Espresso", PumpRunKind.Label(PumpRunKind.Espresso));
    }
}
