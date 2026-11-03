using System.Net;
using Xunit;

namespace MaraxTray.Tests;

public class MachineTrackerTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    private readonly MachineTracker tracker = new(new TraySettings());
    private DateTime now = Start;

    private static MaraxPacket Online(int hx, int steam = 124, int? target = 124, bool pump = false) =>
        new() { Online = true, HxTemp = hx, SteamTemp = steam, SteamTarget = target, Pump = pump };

    private void Send(MaraxPacket packet, int times = 1)
    {
        for (int i = 0; i < times; i++)
        {
            now = now.AddSeconds(1);
            tracker.Update(packet, IPAddress.Loopback, now);
        }
    }

    private MachinePhase Phase => tracker.Current(now).Phase;

    [Fact]
    public void WithoutPacketsThereIsNoData()
    {
        Assert.Equal(MachinePhase.NoData, Phase);
    }

    [Fact]
    public void ColdMachineIsHeating()
    {
        Send(Online(hx: 40, steam: 60));

        Assert.Equal(MachinePhase.Heating, Phase);
    }

    [Theory]
    [InlineData(89, 124)]
    [InlineData(95, 121)]
    public void BothTemperaturesHaveToBeReached(int hx, int steam)
    {
        Send(Online(hx, steam), times: 10);

        Assert.Equal(MachinePhase.Heating, Phase);
    }

    [Fact]
    public void ReadyAtTheThresholds()
    {
        Send(Online(hx: 90, steam: 122), times: 3);

        Assert.Equal(MachinePhase.Ready, Phase);
    }

    [Fact]
    public void OneGarbledPacketDoesNotCountAsReady()
    {
        Send(Online(hx: 85, steam: 124), times: 5);
        Send(Online(hx: 95, steam: 124));
        Send(Online(hx: 85, steam: 124), times: 5);

        Assert.Equal(MachinePhase.Heating, Phase);
    }

    [Fact]
    public void StaysReadyThroughASmallTemperatureDrop()
    {
        Send(Online(hx: 93), times: 3);

        Send(Online(hx: 80, steam: 115));

        Assert.Equal(MachinePhase.Ready, Phase);
    }

    [Fact]
    public void HeatsAgainBelowTheCoolDownTemperature()
    {
        Send(Online(hx: 93), times: 3);

        Send(Online(hx: 79));

        Assert.Equal(MachinePhase.Heating, Phase);
    }

    [Fact]
    public void WithoutTargetTheMachineNeverCountsAsReady()
    {
        Send(Online(hx: 95, steam: 124, target: null), times: 10);

        Assert.Equal(MachinePhase.Heating, Phase);
    }

    [Fact]
    public void MachineOffWhenTheEspHearsNothing()
    {
        Send(new MaraxPacket { Online = false, RawLine = "" });

        Assert.Equal(MachinePhase.MachineOff, Phase);
    }

    [Fact]
    public void UnknownFormatWhenTheEspHearsLinesItCannotRead()
    {
        Send(new MaraxPacket { Online = false, RawLine = "Z,unknown,format" });

        Assert.Equal(MachinePhase.UnknownFormat, Phase);
    }

    [Fact]
    public void SwitchingOffForgetsThatItWasReady()
    {
        Send(Online(hx: 93), times: 3);
        Send(new MaraxPacket { Online = false, RawLine = "" });

        Send(Online(hx: 93));

        Assert.Equal(MachinePhase.Heating, Phase);
    }

    [Fact]
    public void NoDataAfterTheTimeout()
    {
        Send(Online(hx: 93), times: 3);

        now = now.AddSeconds(10);
        Assert.Equal(MachinePhase.Ready, Phase);

        now = now.AddSeconds(1);
        Assert.Equal(MachinePhase.NoData, Phase);
    }

    [Fact]
    public void WaterIsMissingWhenTheTargetStaysAwayWithoutThePump()
    {
        Send(Online(hx: 30, target: null), times: 20);
        Assert.False(tracker.Current(now).WaterMissing);

        Send(Online(hx: 30, target: null));

        Assert.True(tracker.Current(now).WaterMissing);
    }

    // seen on a real machine: "D07" instead of the target while the pump fills the boiler
    [Fact]
    public void FillingTheBoilerAfterPowerOnIsNoWaterAlarm()
    {
        Send(Online(hx: 22, target: null), times: 4);
        Send(Online(hx: 22, target: null, pump: true), times: 60);

        Assert.False(tracker.Current(now).WaterMissing);
    }

    [Fact]
    public void ACodeRightAfterThePumpStoppedStartsTheWaitAnew()
    {
        Send(Online(hx: 22, target: null), times: 15);
        Send(Online(hx: 22, target: null, pump: true));
        Send(Online(hx: 22, target: null), times: 15);

        Assert.False(tracker.Current(now).WaterMissing);
    }

    [Fact]
    public void WaterWarningEndsWithTheTargetBeingBack()
    {
        Send(Online(hx: 30, target: null), times: 21);

        Send(Online(hx: 30));

        Assert.False(tracker.Current(now).WaterMissing);
    }

    [Fact]
    public void WaterWarningEndsWithTheMachine()
    {
        Send(Online(hx: 30, target: null), times: 21);

        Send(new MaraxPacket { Online = false, RawLine = "" });

        Assert.False(tracker.Current(now).WaterMissing);
    }

    [Fact]
    public void PumpFollowsThePacket()
    {
        Send(Online(hx: 93, pump: true));
        Assert.True(tracker.Current(now).Pump);

        Send(Online(hx: 93, pump: false));
        Assert.False(tracker.Current(now).Pump);
    }

    [Fact]
    public void PumpIsNotReportedWithoutData()
    {
        Send(Online(hx: 93, pump: true));

        now = now.AddSeconds(30);

        Assert.False(tracker.Current(now).Pump);
    }
}
